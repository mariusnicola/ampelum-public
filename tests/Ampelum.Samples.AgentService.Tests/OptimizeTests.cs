using System.Net;
using System.Reflection;
using System.Text.Json;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Host.Contracts;
using SignalGrid.Ampelum.Host.ModuleRegistry;
using SignalGrid.Ampelum.Optimizers;
using Xunit;

namespace Ampelum.Samples.AgentService.Tests;

/// <summary>
/// The optimize endpoints are plumbing: the Host's metric scores everything 1.0, so a run collects the
/// module's own answers as demonstrations and tunes nothing.
/// </summary>
public sealed class OptimizeTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(20);

    static async Task<ServiceFixture> StartAsync()
    {
        var service = new ServiceFixture();
        await service.InitializeAsync();
        return service;
    }

    static DemoExample Example(string question, string label) => new(
        new Dictionary<string, JsonElement> { ["Question"] = JsonSerializer.SerializeToElement(question) },
        new Dictionary<string, JsonElement> { ["Answer"] = JsonSerializer.SerializeToElement(label) });

    static OptimizeRequest Request(params DemoExample[] examples) => new(
        OptimizerTypes.BootstrapFewShot, new InlineExamples(examples), new Dictionary<string, JsonElement>());

    static OptimizeRequest TwoExamples() =>
        Request(Example("Where is my parcel?", "Label one."), Example("When does it arrive?", "Label two."));

    /// <summary>
    /// Starts a run, holds it at its first model call, subscribes to its stream, then lets it go, so the stream
    /// carries the live frames and not the ones the Host rebuilds for a finished run.
    /// </summary>
    static async Task<(OptimizeRunHandle Handle, IReadOnlyList<StreamFrame> Frames)> StreamWhileRunningAsync(
        ServiceFixture service, OptimizeRequest request)
    {
        service.CloseGate();
        var handle = await service.Client.StartOptimizeAsync(AgentServiceHost.ModuleName, request, Ct);
        await service.ModelReached.WaitAsync(RunLimit, Ct);

        var streaming = service.Client.StreamOptimizeAsync(handle.RunId, Ct);
        // The run is held at its first model call until the stream's subscriber is registered, so no frame is lost.
        await service.ProgressSubscribed.WaitAsync(RunLimit, Ct);
        service.OpenGate();
        return (handle, await streaming.WaitAsync(RunLimit, Ct));
    }

    [Fact]
    public async Task Optimize_RunsToSuccess_AndLeavesAChallengerVersion()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Model answer one.").EnqueueAnswer("Model answer two.");

        service.CloseGate();
        var handle = await service.Client.StartOptimizeAsync(AgentServiceHost.ModuleName, TwoExamples(), Ct);
        await service.ModelReached.WaitAsync(RunLimit, Ct);
        var waiting = service.Client.WaitForOptimizeAsync(handle.RunId, RunLimit, Ct);
        // The run is held at its first model call, so a wait that returns now returned too early.
        await Task.WhenAny(waiting, Task.Delay(300, Ct));
        waiting.IsCompleted.ShouldBeFalse();
        service.OpenGate();
        var status = await waiting;

        handle.ModuleName.ShouldBe(AgentServiceHost.ModuleName);
        status.RunId.ShouldBe(handle.RunId);
        status.State.ShouldBe(OptimizeRunState.Succeeded, status.Error);
        status.OptimizerType.ShouldBe(OptimizerTypes.BootstrapFewShot);
        status.TrialsCompleted.ShouldBe(2);
        status.TrialsTotal.ShouldBe(2);
        status.ResultPromptVersionId.ShouldNotBeNullOrEmpty();
        status.CompletedAt.ShouldNotBeNull();
        service.Model.RecordedCalls.Count.ShouldBe(2);
        service.SentText(0).ShouldContain("Where is my parcel?");
        service.SentText(1).ShouldContain("When does it arrive?");
        var versions = (await service.Client.ListPromptVersionsAsync(AgentServiceHost.ModuleName, Ct)).Versions;
        versions.ShouldContain(v => v.VersionId == status.ResultPromptVersionId && v.Status == PromptStatus.Challenger);
    }

    [Fact]
    public async Task OptimizeStream_OfAFinishedRun_ReplaysFinalThenDone()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Model answer one.").EnqueueAnswer("Model answer two.");
        var handle = await service.Client.StartOptimizeAsync(AgentServiceHost.ModuleName, TwoExamples(), Ct);
        var status = await service.Client.WaitForOptimizeAsync(handle.RunId, RunLimit, Ct);

        var frames = await service.Client.StreamOptimizeAsync(handle.RunId, Ct);

        frames.Select(f => f.Event).ShouldBe(["final", "done"]);
        frames[0].Data.GetProperty("versionId").GetString().ShouldBe(status.ResultPromptVersionId);
    }

    [Fact]
    public async Task OptimizeStream_OfARunInFlight_SendsOneProgressFramePerExampleThenFinalAndDone()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Model answer one.").EnqueueAnswer("Model answer two.");

        var (handle, frames) = await StreamWhileRunningAsync(service, TwoExamples());

        frames.Select(f => f.Event).ShouldBe(["progress", "progress", "final", "done"]);
        frames[0].Data.GetProperty("runId").GetString().ShouldBe(handle.RunId);
        frames.Take(2).Select(f => f.Data.GetProperty("trialsCompleted").GetInt32()).ShouldBe([1, 2]);
        frames[1].Data.GetProperty("trialsTotal").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Optimize_RefusesAnUnknownModuleAnUnknownOptimizerAndAnUnknownRun()
    {
        await using var service = await StartAsync();

        var module = await Should.ThrowAsync<AgentServiceException>(() => service.Client.StartOptimizeAsync("Missing", TwoExamples(), Ct));
        var optimizer = await Should.ThrowAsync<AgentServiceException>(() => service.Client.StartOptimizeAsync(
            AgentServiceHost.ModuleName, TwoExamples() with { OptimizerType = "NotAnOptimizer" }, Ct));
        var status = await Should.ThrowAsync<AgentServiceException>(() => service.Client.GetOptimizeStatusAsync("run-missing", Ct));
        var stream = await Should.ThrowAsync<AgentServiceException>(() => service.Client.StreamOptimizeAsync("run-missing", Ct));

        module.Error!.Code.ShouldBe(ErrorCodes.ModuleNotFound);
        optimizer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        status.Status.ShouldBe(HttpStatusCode.NotFound);
        status.Error!.Code.ShouldBe(ErrorCodes.RunNotFound);
        stream.Error!.Code.ShouldBe(ErrorCodes.RunNotFound);
        service.Model.RecordedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task KnownLimit_OptimizeKeepsTheModelsAnswerWhateverTheLabelSays()
    {
        await using var service = await StartAsync();
        service.EnqueueAnswer("Completely different from the label.");
        var request = Request(Example("Where is my parcel?", "The label the answer should have matched."));

        var (_, frames) = await StreamWhileRunningAsync(service, request);

        // Nothing was scored against the label: the demonstration holds the model's answer.
        var final = frames.Single(f => f.Event == "final").Data;
        var demos = final.GetProperty("demoAssignments").EnumerateObject().Single().Value.EnumerateArray().ToArray();
        demos.Length.ShouldBe(1);
        demos[0].GetProperty("outputs").GetProperty("Answer").GetString().ShouldBe("Completely different from the label.");
    }

    [Fact]
    public void KnownLimit_TheHostsOptimizeMetricScoresEveryPairOne()
    {
        // The metric is a private class of the Host's module adapter, so reflection is the only way to reach it.
        var type = typeof(PredictComposedAdapter<AnswerSignature>).GetNestedType("NoOpMetric", BindingFlags.NonPublic);
        type.ShouldNotBeNull("the Host's metric type was renamed or removed; this pin must be revisited");
        var metric = (IMetric)Activator.CreateInstance(type.MakeGenericType(typeof(AnswerSignature)), nonPublic: true)!;
        var signature = SignatureMetadataCache.For<AnswerSignature>();

        var matching = metric.Score("same", "same", signature);
        var opposite = metric.Score("this", "nothing like it", signature);

        matching.ShouldBe(1.0);
        opposite.ShouldBe(1.0);
    }
}
