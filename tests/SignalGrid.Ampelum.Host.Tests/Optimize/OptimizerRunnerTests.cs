#pragma warning disable AMPELUM_V09_PREVIEW
using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Optimize;
using SignalGrid.Ampelum.Host.Persistence;
using SignalGrid.Ampelum.Host.Tests.Fixtures;
using Xunit;
using HostRegistry = SignalGrid.Ampelum.Host.ModuleRegistry.ModuleRegistry;

namespace SignalGrid.Ampelum.Host.Tests.Optimize;

public sealed class OptimizerRunnerTests : IDisposable
{
    readonly TempStorageFixture _storage = new();

    public void Dispose() => _storage.Dispose();

    (OptimizerRunner runner, IOptimizerRunStore runStore, IPromptVersionStore versionStore,
        OptimizerProgressBus bus, FakeChatClient chatClient, IModuleRegistry registry)
        MakeRunner(string? moduleName = "Fake")
    {
        var connFactory = TestData.Factory(_storage.Options);
        var runStore = new OptimizerRunStore(connFactory);
        var versionStore = new PromptVersionStore(connFactory);

        var chatClient = new FakeChatClient();
        var ctx = new AmpelumContext(chatClient, new ChatAdapter(), NullTraceSink.Instance,
            NullLoggerFactory.Instance);
        var predict = new Predict<FakeSig>(ctx);
        var adapter = new PredictComposedAdapter<FakeSig>(predict);

        var desc = new ModuleDescriptor(
            moduleName!, typeof(FakeSig).FullName!, "Test module",
            [], [], false);

        var registry = new HostRegistry();
        if (moduleName is not null)
            registry.Register(new ModuleSlot(moduleName, adapter, desc));
        registry.Freeze();

        var bus = new OptimizerProgressBus();

        var httpFactory = new ServiceCollection().AddHttpClient()
            .BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        var runner = new OptimizerRunner(
            registry, runStore, bus, versionStore,
            Options.Create(_storage.Options),
            httpFactory,
            NullLogger<OptimizerRunner>.Instance);

        return (runner, runStore, versionStore, bus, chatClient, registry);
    }

    // The bus only delivers to subscribers registered at publish time. Registration happens
    // synchronously inside the first MoveNextAsync, so start it here before returning.
    static (List<SseEnvelope> Events, Task Completed) StartSubscriber(OptimizerProgressBus bus, string runId)
    {
        var events = new List<SseEnvelope>();
        var enumerator = bus.Subscribe(runId, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var firstMove = enumerator.MoveNextAsync();
        var completed = Task.Run(async () =>
        {
            try
            {
                var has = await firstMove;
                while (has)
                {
                    events.Add(enumerator.Current);
                    has = await enumerator.MoveNextAsync();
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }, TestContext.Current.CancellationToken);
        return (events, completed);
    }
    static async Task WaitForTerminalStateAsync(
        IOptimizerRunStore store, string runId, CancellationToken ct)
    {
        while (true)
        {
            var row = await store.TryGetAsync(runId, ct);
            if (row is not null && OptimizeRunState.All.Contains(row.State) &&
                row.State is OptimizeRunState.Succeeded or OptimizeRunState.Failed or OptimizeRunState.Cancelled)
                return;
            await Task.Delay(20, ct);
        }
    }

    [Fact]
    public async Task InlineExamples_HappyPath_RowSucceeded_FileWritten_VersionInserted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, runStore, versionStore, _, chatClient, _) = MakeRunner();

        // Enqueue 2 LM responses (one per example in the trainset)
        chatClient.EnqueueText("[[ ## A ## ]]\nAnswer1");
        chatClient.EnqueueText("[[ ## A ## ]]\nAnswer2");

        var runId = "run-inline-1";
        var exInputs = new Dictionary<string, JsonElement>
            { ["Q"] = JsonSerializer.SerializeToElement("Hello") };
        var exOutputs = new Dictionary<string, JsonElement>
            { ["A"] = JsonSerializer.SerializeToElement("World") };
        var examples = new List<DemoExample>
        {
            new(exInputs, exOutputs),
            new(exInputs, exOutputs),
        };
        var request = new OptimizeRequest(
            OptimizerType: OptimizerTypes.BootstrapFewShot,
            TrainingSet: new InlineExamples(examples),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        var row = new OptimizerRunRow(runId, "Fake", OptimizerTypes.BootstrapFewShot,
            OptimizeRunState.Queued, DateTimeOffset.UtcNow, null, 0, null, null, null);
        await runStore.InsertAsync(row, ct);
        await runner.EnqueueAsync(runId, request, ct);

        await WaitForTerminalStateAsync(runStore, runId, ct)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);

        var finalRow = await runStore.TryGetAsync(runId, ct);
        finalRow.ShouldNotBeNull();
        finalRow.State.ShouldBe(OptimizeRunState.Succeeded);
        finalRow.ResultVersionId.ShouldNotBeNullOrEmpty();

        var versions = await versionStore.ListByModuleAsync("Fake", ct);
        versions.Count.ShouldBeGreaterThanOrEqualTo(1);

        var promptDir = _storage.PromptDir;
        var files = Directory.Exists(promptDir)
            ? Directory.GetFiles(promptDir, "*.json")
            : [];
        files.Length.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task FileExamples_ReadAndProcess_RowSucceeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, runStore, _, _, chatClient, _) = MakeRunner();

        chatClient.EnqueueText("[[ ## A ## ]]\nFileAnswer");

        var jsonlPath = Path.Combine(_storage.TempDir, "examples.jsonl");
        var demo = new DemoExample(
            new Dictionary<string, JsonElement> { ["Q"] = JsonSerializer.SerializeToElement("Q1") },
            new Dictionary<string, JsonElement> { ["A"] = JsonSerializer.SerializeToElement("A1") });
        var line = JsonSerializer.Serialize(demo, AmpelumHostContractsJsonContext.Default.DemoExample);
        await File.WriteAllTextAsync(jsonlPath, line + "\n", ct);

        var runId = "run-file-1";
        var row = new OptimizerRunRow(runId, "Fake", OptimizerTypes.BootstrapFewShot,
            OptimizeRunState.Queued, DateTimeOffset.UtcNow, null, 0, null, null, null);
        await runStore.InsertAsync(row, ct);

        var request = new OptimizeRequest(
            OptimizerType: OptimizerTypes.BootstrapFewShot,
            TrainingSet: new FileExamples(jsonlPath),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        await runner.EnqueueAsync(runId, request, ct);
        await WaitForTerminalStateAsync(runStore, runId, ct)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);

        var finalRow = await runStore.TryGetAsync(runId, ct);
        finalRow!.State.ShouldBe(OptimizeRunState.Succeeded);
    }

    [Fact]
    public async Task ModuleNotInRegistry_RowFailed_ErrorPublished()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, runStore, _, bus, _, _) = MakeRunner(moduleName: null);

        var runId = "run-nomod-1";
        var row = new OptimizerRunRow(runId, "NonExistent", OptimizerTypes.BootstrapFewShot,
            OptimizeRunState.Queued, DateTimeOffset.UtcNow, null, 0, null, null, null);
        await runStore.InsertAsync(row, ct);

        var (events, subTask) = StartSubscriber(bus, runId);

        var request = new OptimizeRequest(
            OptimizerType: OptimizerTypes.BootstrapFewShot,
            TrainingSet: new InlineExamples(new List<DemoExample>()),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        await runner.EnqueueAsync(runId, request, ct);
        await WaitForTerminalStateAsync(runStore, runId, ct)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);
        await subTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var finalRow = await runStore.TryGetAsync(runId, ct);
        finalRow!.State.ShouldBe(OptimizeRunState.Failed);
        finalRow.ErrorMessage.ShouldNotBeNullOrEmpty();
        events.ShouldContain(e => e.EventName == SseEventNames.Error);
        events.ShouldContain(e => e.EventName == SseEventNames.Done);
    }

    [Fact]
    public async Task ProgressEvents_PublishedForEachTrial()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, runStore, _, bus, chatClient, _) = MakeRunner();

        chatClient.EnqueueText("[[ ## A ## ]]\nProg1");
        chatClient.EnqueueText("[[ ## A ## ]]\nProg2");

        var runId = "run-progress-1";
        var row = new OptimizerRunRow(runId, "Fake", OptimizerTypes.BootstrapFewShot,
            OptimizeRunState.Queued, DateTimeOffset.UtcNow, null, 0, null, null, null);
        await runStore.InsertAsync(row, ct);

        var (events, subTask) = StartSubscriber(bus, runId);

        var exInputs = new Dictionary<string, JsonElement>
            { ["Q"] = JsonSerializer.SerializeToElement("Q") };
        var exOutputs = new Dictionary<string, JsonElement>
            { ["A"] = JsonSerializer.SerializeToElement("A") };
        var request = new OptimizeRequest(
            OptimizerType: OptimizerTypes.BootstrapFewShot,
            TrainingSet: new InlineExamples(new List<DemoExample>
            {
                new(exInputs, exOutputs),
                new(exInputs, exOutputs),
            }),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        await runner.EnqueueAsync(runId, request, ct);
        await WaitForTerminalStateAsync(runStore, runId, ct)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);
        await subTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var finalRow = await runStore.TryGetAsync(runId, ct);
        finalRow.ShouldNotBeNull();
        finalRow.TrialsCompleted.ShouldBe(2);
        finalRow.TrialsTotal.ShouldBe(2);
        events.ShouldContain(e => e.EventName == SseEventNames.Progress);
        events.ShouldContain(e => e.EventName == SseEventNames.Final);
        events.ShouldContain(e => e.EventName == SseEventNames.Done);
    }

    [Fact]
    public async Task ConcurrentRuns_TwoModules_BothSucceed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, runStore, _, _, chatClient, _) = MakeRunner();

        chatClient.EnqueueText("[[ ## A ## ]]\nA1");
        chatClient.EnqueueText("[[ ## A ## ]]\nA2");

        var ex = new DemoExample(
            new Dictionary<string, JsonElement> { ["Q"] = JsonSerializer.SerializeToElement("X") },
            new Dictionary<string, JsonElement> { ["A"] = JsonSerializer.SerializeToElement("Y") });
        var request = new OptimizeRequest(
            OptimizerType: OptimizerTypes.BootstrapFewShot,
            TrainingSet: new InlineExamples(new List<DemoExample> { ex }),
            OptimizerOptions: new Dictionary<string, JsonElement>());

        var runId1 = "run-conc-1";
        var runId2 = "run-conc-2";
        await runStore.InsertAsync(new OptimizerRunRow(runId1, "Fake", OptimizerTypes.BootstrapFewShot,
            OptimizeRunState.Queued, DateTimeOffset.UtcNow, null, 0, null, null, null), ct);
        await runStore.InsertAsync(new OptimizerRunRow(runId2, "Fake", OptimizerTypes.BootstrapFewShot,
            OptimizeRunState.Queued, DateTimeOffset.UtcNow, null, 0, null, null, null), ct);

        await runner.EnqueueAsync(runId1, request, ct);
        await runner.EnqueueAsync(runId2, request, ct);

        await Task.WhenAll(
            WaitForTerminalStateAsync(runStore, runId1, ct),
            WaitForTerminalStateAsync(runStore, runId2, ct))
            .WaitAsync(TimeSpan.FromSeconds(15), ct);

        var r1 = await runStore.TryGetAsync(runId1, ct);
        var r2 = await runStore.TryGetAsync(runId2, ct);
        r1!.State.ShouldBeOneOf(OptimizeRunState.Succeeded, OptimizeRunState.Failed);
        r2!.State.ShouldBeOneOf(OptimizeRunState.Succeeded, OptimizeRunState.Failed);
    }
}
