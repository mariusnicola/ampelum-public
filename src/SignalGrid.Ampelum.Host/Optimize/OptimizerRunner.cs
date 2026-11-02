#pragma warning disable AMPELUM_V09_PREVIEW
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Optimizers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.Internal;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Host.Persistence;

namespace SignalGrid.Ampelum.Host.Optimize;

internal sealed class OptimizerRunner : IOptimizerRunner
{
    readonly IModuleRegistry _registry;
    readonly IOptimizerRunStore _runStore;
    readonly IOptimizerProgressBus _bus;
    readonly IPromptVersionStore _versionStore;
    readonly IOptions<AmpelumHostOptions> _opts;
    readonly IHttpClientFactory _http;
    readonly ILogger<OptimizerRunner> _log;

    public OptimizerRunner(
        IModuleRegistry registry,
        IOptimizerRunStore runStore,
        IOptimizerProgressBus bus,
        IPromptVersionStore versionStore,
        IOptions<AmpelumHostOptions> opts,
        IHttpClientFactory http,
        ILogger<OptimizerRunner> log)
    {
        _registry = registry;
        _runStore = runStore;
        _bus = bus;
        _versionStore = versionStore;
        _opts = opts;
        _http = http;
        _log = log;
    }

    public ValueTask EnqueueAsync(string runId, OptimizeRequest request, CancellationToken ct)
    {
        _ = Task.Run(() => ExecuteRunAsync(runId, request, CancellationToken.None));
        return ValueTask.CompletedTask;
    }

    async Task ExecuteRunAsync(string runId, OptimizeRequest request, CancellationToken ct)
    {
        try
        {
            var runRow = await _runStore.TryGetAsync(runId, ct)
                ?? throw new InvalidOperationException($"Run '{runId}' not found in store.");

            await _runStore.UpdateProgressAsync(runId, 0, null, ct);

            var demoExamples = await MaterializeAsync(request.TrainingSet, ct);
            var trainset = demoExamples
                .Select(e => new Example(
                    JsonElementUnboxer.Unbox(e.Inputs),
                    JsonElementUnboxer.Unbox(e.Outputs)))
                .ToList();

            if (!_registry.TryGet(runRow.ModuleName, out var slot))
                throw new KeyNotFoundException($"Module '{runRow.ModuleName}' not found in registry.");

            if (slot.Current is not IOptimizableModule optimizable)
                throw new InvalidOperationException(
                    $"Module '{runRow.ModuleName}' does not support optimization.");

            long eventId = 0;
            var latestProgress = new OptimizerProgress("starting", 0, 0, 0d);
            var progress = new SynchronousProgress<OptimizerProgress>(p =>
            {
                latestProgress = p;
                var payload = JsonSerializer.Serialize(
                    new SseProgressPayload(runId, p.Step, p.TotalSteps),
                    AmpelumHostJsonContext.Default.SseProgressPayload);
                _bus.Publish(runId, new SseEnvelope(SseEventNames.Progress, payload,
                    Interlocked.Increment(ref eventId)));
            });

            (IReadOnlyDictionary<string, IReadOnlyList<Example>> demosBySubmodule,
             IReadOnlyDictionary<string, string> instructionOverrides) =
                await optimizable.RunBootstrapAsync(trainset, progress, ct);

            await _runStore.UpdateProgressAsync(
                runId, latestProgress.Step, latestProgress.TotalSteps, ct);

            var versionId = MintVersionId();
            var promptDir = _opts.Value.Storage.PromptDir;
            Directory.CreateDirectory(promptDir);
            var filePath = Path.Combine(promptDir, $"{runRow.ModuleName}-{versionId}.json");

            var demosForFile = demosBySubmodule.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<DemoExampleShape>)kv.Value
                    .Select(ex => new DemoExampleShape(
                        JsonElementUnboxer.Box(ex.Inputs),
                        JsonElementUnboxer.Box(ex.Outputs)))
                    .ToList());

            var demoAssignmentsForPv = demosBySubmodule.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<DemoExample>)kv.Value
                    .Select(ex => new DemoExample(
                        JsonElementUnboxer.Box(ex.Inputs),
                        JsonElementUnboxer.Box(ex.Outputs)))
                    .ToList());

            var shape = new PromptVersionFileShape(
                Version: 1,
                Meta: new PromptMeta(
                    VersionId: versionId,
                    ModuleName: runRow.ModuleName,
                    Status: PromptStatus.Challenger,
                    CreatedAt: DateTimeOffset.UtcNow,
                    ParentVersionId: null,
                    Note: null),
                TProgramType: slot.Descriptor.SignatureType,
                DemoAssignments: demosForFile,
                InstructionOverrides: instructionOverrides);

            await PromptVersionFileReader.WriteAsync(filePath, shape, ct);

            var versionRow = new PromptVersionRow(
                VersionId: versionId,
                ModuleName: runRow.ModuleName,
                Status: PromptStatus.Challenger,
                CreatedAt: DateTimeOffset.UtcNow,
                ParentVersionId: null,
                FilePath: filePath,
                Note: null);
            await _versionStore.InsertChallengerAsync(versionRow, ct);

            var pv = new PromptVersion(versionId, runRow.ModuleName, DateTimeOffset.UtcNow,
                PromptStatus.Challenger, demoAssignmentsForPv, instructionOverrides, null, null);
            var finalPayload = JsonSerializer.Serialize(pv, AmpelumHostJsonContext.Default.PromptVersion);
            _bus.Publish(runId, new SseEnvelope(SseEventNames.Final, finalPayload,
                Interlocked.Increment(ref eventId)));
            _bus.Publish(runId, new SseEnvelope(SseEventNames.Done, "{}",
                Interlocked.Increment(ref eventId)));

            await _runStore.CompleteAsync(runId, OptimizeRunState.Succeeded, versionId, null, ct);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Optimizer run {RunId} cancelled.", runId);
            await SafeCompleteAsync(runId, OptimizeRunState.Cancelled, null,
                "Cancelled.", "{\"reason\":\"cancelled\"}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Optimizer run {RunId} failed.", runId);
            var errMsg = JsonSerializer.Serialize(new { reason = ex.Message });
            await SafeCompleteAsync(runId, OptimizeRunState.Failed, null, ex.ToString(), errMsg);
        }
        finally
        {
            _bus.Complete(runId);
        }
    }

    async Task SafeCompleteAsync(
        string runId, string state, string? versionId, string? errorMsg, string errorPayload)
    {
        try
        {
            _bus.Publish(runId, new SseEnvelope(SseEventNames.Error, errorPayload, 0));
            _bus.Publish(runId, new SseEnvelope(SseEventNames.Done, "{}", 0));
            await _runStore.CompleteAsync(runId, state, versionId, errorMsg, CancellationToken.None);
        }
        catch (Exception ex2)
        {
            _log.LogError(ex2, "Failed to finalize run store for run {RunId}.", runId);
        }
    }

    async Task<IReadOnlyList<DemoExample>> MaterializeAsync(ExampleSource source, CancellationToken ct)
    {
        return source switch
        {
            InlineExamples inline => inline.Examples,
            FileExamples file    => await ReadFileExamplesAsync(file.Path, ct),
            RemoteExamples remote => await FetchRemoteExamplesAsync(remote, ct),
            _ => throw new NotSupportedException(
                $"Unsupported ExampleSource type: {source.GetType().Name}"),
        };
    }

    static async Task<IReadOnlyList<DemoExample>> ReadFileExamplesAsync(
        string path, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        var result = new List<DemoExample>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ex = JsonSerializer.Deserialize(line,
                AmpelumHostContractsJsonContext.Default.DemoExample)
                ?? throw new JsonException($"Null DemoExample deserialized from line.");
            result.Add(ex);
        }
        return result;
    }

    async Task<IReadOnlyList<DemoExample>> FetchRemoteExamplesAsync(
        RemoteExamples remote, CancellationToken ct)
    {
        var http = _http.CreateClient();
        var bytes = await http.GetByteArrayAsync(remote.Uri, ct);

        if (remote.Sha256 is not null)
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, remote.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for remote examples: expected '{remote.Sha256}', got '{actual}'.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<DemoExample>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ex = JsonSerializer.Deserialize(line,
                AmpelumHostContractsJsonContext.Default.DemoExample)
                ?? throw new JsonException("Null DemoExample deserialized.");
            result.Add(ex);
        }
        return result;
    }

    static string MintVersionId()
    {
        var now = DateTimeOffset.UtcNow;
        var guid6 = Guid.NewGuid().ToString("N")[..6];
        return $"v-{now:yyyyMMdd-HHmmss-fff}-{guid6}";
    }

    sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
