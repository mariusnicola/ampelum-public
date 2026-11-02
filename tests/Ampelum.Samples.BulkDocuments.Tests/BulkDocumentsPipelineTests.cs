using System.Diagnostics;
using Microsoft.Extensions.AI;
using Shouldly;
using SignalGrid.Ampelum.Batch;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.RateLimiting;
using SignalGrid.Ampelum.Resilience;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.BulkDocuments.Tests;

public sealed class BulkDocumentsPipelineTests
{
    const string BatchModel = "model-under-test";

    static Document Doc(int n) => new($"d{n}", $"Title {n}", $"Text number {n}");

    sealed class Rig : IDisposable
    {
        readonly string _cacheDirectory;
        readonly bool _ownsCache;

        public Rig(
            Func<BulkDocumentsOptions, BulkDocumentsOptions>? tweak = null,
            FakeAnthropicBatchHandler? batchHandler = null,
            string? cacheDirectory = null)
        {
            _ownsCache = cacheDirectory is null;
            _cacheDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), $"ampelum-bulk-test-{Guid.NewGuid():N}");
            Handler = batchHandler ?? new FakeAnthropicBatchHandler();
            var options = new BulkDocumentsOptions { BatchModel = BatchModel, PollInterval = TimeSpan.FromMilliseconds(1) };
            options = tweak?.Invoke(options) ?? options;
            Options = options;
            Pipeline = new BulkDocumentsPipeline(
                new AnthropicBatchSubmitter(new HttpClient(Handler), "placeholder-key"),
                Endpoints.All,
                new FileDistributedCache(_cacheDirectory),
                options);
        }

        public FakeEndpoints Endpoints { get; } = new();
        public FakeAnthropicBatchHandler Handler { get; }
        public BulkDocumentsOptions Options { get; }
        public BulkDocumentsPipeline Pipeline { get; }

        public void Dispose()
        {
            Pipeline.Dispose();
            if (_ownsCache && Directory.Exists(_cacheDirectory))
                Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Batch_CarriesTheConfiguredModelAndMapsResultsBackByCustomId()
    {
        using var rig = new Rig();
        var docs = new[] { Doc(1), Doc(2), Doc(3) };

        var summaries = await rig.Pipeline.SummarizeBatchAsync(docs, TestContext.Current.CancellationToken);

        rig.Handler.Submissions.Count.ShouldBe(1);
        var requests = rig.Handler.Submissions[0]["requests"]!.AsArray();
        requests.Count.ShouldBe(3);
        foreach (var request in requests)
        {
            request!["params"]!["model"]!.GetValue<string>().ShouldBe(BatchModel);
            request["params"]!["max_tokens"]!.GetValue<int>().ShouldBe(rig.Options.MaxSummaryTokens);
        }
        // The fake returns results in reverse order, so only the custom id can map them back.
        summaries.Select(s => s.DocumentId).ToArray().ShouldBe(["d1", "d2", "d3"]);
        foreach (var doc in docs)
            summaries.Single(s => s.DocumentId == doc.Id).Summary.ShouldBe($"Summary: {doc.Text}");
    }

    [Fact]
    public async Task Batch_WaitsForTheBatchToEndBeforeReadingResults()
    {
        var handler = new FakeAnthropicBatchHandler(pollsBeforeEnded: 3);
        using var rig = new Rig(batchHandler: handler);

        await rig.Pipeline.SummarizeBatchAsync([Doc(1)], TestContext.Current.CancellationToken);

        // create, four status checks (three in progress, one ended), results
        handler.Calls.ShouldBe(6);
    }

    [Fact]
    public async Task Batch_GivesUpWithATimeoutWhenTheBatchNeverEnds()
    {
        var handler = new FakeAnthropicBatchHandler(pollsBeforeEnded: 50);
        using var rig = new Rig(o => o with { MaxPolls = 2 }, handler);

        await Should.ThrowAsync<TimeoutException>(
            async () => await rig.Pipeline.SummarizeBatchAsync([Doc(1)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Batch_ResubmitsOnlyTheDocumentThatFailed()
    {
        var handler = new FakeAnthropicBatchHandler().FailDocument("d2");
        using var rig = new Rig(batchHandler: handler);

        var summaries = await rig.Pipeline.SummarizeBatchAsync([Doc(1), Doc(2), Doc(3)], TestContext.Current.CancellationToken);

        handler.Submissions.Count.ShouldBe(2);
        var second = handler.Submissions[1]["requests"]!.AsArray();
        second.Count.ShouldBe(1);
        second[0]!["custom_id"]!.GetValue<string>().ShouldBe("d2");
        summaries.ShouldAllBe(s => s.Summary != null && s.Error == null);
    }

    [Fact]
    public async Task Batch_KeepsTheErrorOfADocumentThatNeverSucceeds()
    {
        var handler = new FakeAnthropicBatchHandler().AlwaysFailDocument("d2");
        using var rig = new Rig(batchHandler: handler);

        var summaries = await rig.Pipeline.SummarizeBatchAsync([Doc(1), Doc(2)], TestContext.Current.CancellationToken);

        handler.Submissions.Count.ShouldBe(rig.Options.BatchAttempts);
        summaries.Single(s => s.DocumentId == "d1").Summary.ShouldNotBeNull();
        var failed = summaries.Single(s => s.DocumentId == "d2");
        failed.Summary.ShouldBeNull();
        failed.Error.ShouldBe("overloaded");
    }

    [Fact]
    public async Task Batch_WithASingleAttemptDoesNotRetry()
    {
        var handler = new FakeAnthropicBatchHandler().FailDocument("d2");
        using var rig = new Rig(o => o with { BatchAttempts = 1 }, handler);

        var summaries = await rig.Pipeline.SummarizeBatchAsync([Doc(1), Doc(2)], TestContext.Current.CancellationToken);

        handler.Submissions.Count.ShouldBe(1);
        summaries.Single(s => s.DocumentId == "d2").Summary.ShouldBeNull();
    }

    [Fact]
    public async Task SecondIdenticalCall_IsServedFromTheCacheAndNeverReachesTheClient()
    {
        using var rig = new Rig();
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "first answer");

        var first = await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);
        var second = await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);

        first.ShouldBe("first answer");
        second.ShouldBe("first answer");
        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cache_IsExactMatch_ARewordedDocumentMissesAndCallsTheClientAgain()
    {
        using var rig = new Rig();
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "answer one");
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "answer two");
        var original = Doc(1);
        var oneCharLonger = original with { Text = original.Text + "." };

        var first = await rig.Pipeline.SummarizeAsync(original, TestContext.Current.CancellationToken);
        var second = await rig.Pipeline.SummarizeAsync(oneCharLonger, TestContext.Current.CancellationToken);

        first.ShouldBe("answer one");
        second.ShouldBe("answer two");
        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Cache_OnDisk_SurvivesANewPipelineInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ampelum-bulk-test-{Guid.NewGuid():N}");
        try
        {
            using (var first = new Rig(cacheDirectory: directory))
            {
                FakeScript.EnqueueSummary(first.Endpoints.Primary, "stored answer");
                (await first.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken)).ShouldBe("stored answer");
            }

            using var second = new Rig(cacheDirectory: directory);
            var answer = await second.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);

            answer.ShouldBe("stored answer");
            second.Endpoints.Primary.RecordedCalls.Count.ShouldBe(0);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Stream_YieldsPartialUpdatesThenTheFinalSummary_AndARepeatIsCached()
    {
        using var rig = new Rig();
        FakeScript.EnqueueStreamedSummary(rig.Endpoints.Primary, "streamed answer");

        var updates = await Collect(rig.Pipeline.StreamSummaryAsync(Doc(1), TestContext.Current.CancellationToken));
        var repeat = await Collect(rig.Pipeline.StreamSummaryAsync(Doc(1), TestContext.Current.CancellationToken));

        updates.Count.ShouldBeGreaterThan(1);
        updates.Take(updates.Count - 1).ShouldAllBe(u => !u.IsFinal);
        updates[^1].IsFinal.ShouldBeTrue();
        updates[^1].Value!.Summary.ShouldBe("streamed answer");
        repeat[^1].Value!.Summary.ShouldBe("streamed answer");
        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RateLimit_RejectsACallThatCannotGetATokenInTime_WithoutReachingTheClient()
    {
        using var rig = new Rig(o => o with
        {
            RateLimit = new RateLimitingOptions
            {
                RequestsPerMinute = 1,
                BurstMultiplier = 1,
                AcquireTimeout = TimeSpan.FromMilliseconds(300),
            },
        });
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "one");
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "two");
        await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);

        var clock = Stopwatch.StartNew();
        var failure = await Should.ThrowAsync<LmCallException>(
            async () => await rig.Pipeline.SummarizeAsync(Doc(2), TestContext.Current.CancellationToken));
        clock.Stop();

        failure.InnerException.ShouldBeOfType<RateLimitedException>();
        // The call waited for a token until the timeout instead of failing at once.
        clock.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(250);
        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RateLimit_DoesNotChargeCacheHits()
    {
        using var rig = new Rig(o => o with
        {
            RateLimit = new RateLimitingOptions
            {
                RequestsPerMinute = 1,
                BurstMultiplier = 1,
                AcquireTimeout = TimeSpan.FromMilliseconds(100),
            },
        });
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "one");
        await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);

        // The bucket is empty, but the cache sits in front of the limiter.
        var again = await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);

        again.ShouldBe("one");
    }

    [Fact]
    public async Task Failover_AnswersFromTheFallbackWhenThePrimaryFails()
    {
        using var rig = new Rig();
        rig.Endpoints.Primary.EnqueueError(new HttpRequestException("primary down"));
        FakeScript.EnqueueSummary(rig.Endpoints.Fallback, "from fallback");

        var answer = await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);

        answer.ShouldBe("from fallback");
        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(1);
        rig.Endpoints.Fallback.RecordedCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CircuitBreaker_StopsCallingAnEndpointThatKeepsFailing()
    {
        using var rig = new Rig(o => o with
        {
            Breaker = new CircuitBreakerOptions { FailureThreshold = 2, BreakDuration = TimeSpan.FromMinutes(1) },
        });
        rig.Endpoints.Primary.EnqueueError(new HttpRequestException("down"));
        rig.Endpoints.Primary.EnqueueError(new HttpRequestException("down"));
        foreach (var n in new[] { 1, 2, 3 })
            FakeScript.EnqueueSummary(rig.Endpoints.Fallback, $"fallback {n}");

        for (var n = 1; n <= 3; n++)
            (await rig.Pipeline.SummarizeAsync(Doc(n), TestContext.Current.CancellationToken)).ShouldBe($"fallback {n}");

        // Two failures opened the circuit; the third request skipped the primary without calling it.
        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(2);
        rig.Endpoints.Fallback.RecordedCalls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task LoadBalanced_AlternatesOnEveryCall()
    {
        using var rig = new Rig(o => o with { Routing = RoutingMode.LoadBalanced });

        var answers = await AskFourDistinctDocuments(rig);

        answers.ShouldBe(["P1", "F1", "P2", "F2"]);
    }

    [Fact]
    public async Task KeyRotating_StaysOnOneEndpointForTheConfiguredNumberOfCalls()
    {
        using var rig = new Rig(o => o with { Routing = RoutingMode.KeyRotating, KeyRotatingCallsPerKey = 2 });

        var answers = await AskFourDistinctDocuments(rig);

        answers.ShouldBe(["P1", "P2", "F1", "F2"]);
        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(2);
        rig.Endpoints.Fallback.RecordedCalls.Count.ShouldBe(2);
    }

    static async Task<List<string>> AskFourDistinctDocuments(Rig rig)
    {
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "P1");
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "P2");
        FakeScript.EnqueueSummary(rig.Endpoints.Fallback, "F1");
        FakeScript.EnqueueSummary(rig.Endpoints.Fallback, "F2");
        var answers = new List<string>();
        for (var n = 1; n <= 4; n++)
            answers.Add(await rig.Pipeline.SummarizeAsync(Doc(n), TestContext.Current.CancellationToken));
        return answers;
    }

    [Fact]
    public async Task Stream_DoesNotFailOver_WhenThePrimaryCannotStream()
    {
        using var rig = new Rig();
        // The primary has no streaming reply queued, so its stream fails; the fallback could answer.
        FakeScript.EnqueueStreamedSummary(rig.Endpoints.Fallback, "never reached");

        await Should.ThrowAsync<Exception>(
            async () => await Collect(rig.Pipeline.StreamSummaryAsync(Doc(1), TestContext.Current.CancellationToken)));

        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(1);
        rig.Endpoints.Fallback.RecordedCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Batch_KeepsFinishedSummariesWhenALaterAttemptIsCanceled()
    {
        var handler = new FakeAnthropicBatchHandler().FailDocument("d2").CancelBatchNumber(2);
        using var rig = new Rig(batchHandler: handler);

        var summaries = await rig.Pipeline.SummarizeBatchAsync([Doc(1), Doc(2), Doc(3)], TestContext.Current.CancellationToken);

        handler.Submissions.Count.ShouldBe(2);
        summaries.Select(s => s.DocumentId).ToArray().ShouldBe(["d1", "d2", "d3"]);
        summaries.Single(s => s.DocumentId == "d1").Summary.ShouldBe("Summary: Text number 1");
        summaries.Single(s => s.DocumentId == "d3").Summary.ShouldBe("Summary: Text number 3");
        var failed = summaries.Single(s => s.DocumentId == "d2");
        failed.Summary.ShouldBeNull();
        failed.Error.ShouldNotBeNull().ShouldContain("Cancelled");
    }

    [Fact]
    public async Task Batch_KeepsFinishedSummariesWhenALaterSubmitIsRejectedByTheProvider()
    {
        var handler = new FakeAnthropicBatchHandler().FailDocument("d2").RejectSubmitNumber(2, (System.Net.HttpStatusCode)529);
        using var rig = new Rig(batchHandler: handler);

        var summaries = await rig.Pipeline.SummarizeBatchAsync([Doc(1), Doc(2), Doc(3)], TestContext.Current.CancellationToken);

        summaries.Single(s => s.DocumentId == "d1").Summary.ShouldNotBeNull();
        summaries.Single(s => s.DocumentId == "d3").Summary.ShouldNotBeNull();
        var failed = summaries.Single(s => s.DocumentId == "d2");
        failed.Summary.ShouldBeNull();
        failed.Error.ShouldNotBeNull().ShouldContain("529");
    }

    [Fact]
    public async Task Batch_KeepsFinishedSummariesWhenALaterAttemptNeverEnds()
    {
        var handler = new FakeAnthropicBatchHandler().FailDocument("d2").NeverEndBatchNumber(2);
        using var rig = new Rig(o => o with { MaxPolls = 3 }, handler);

        var summaries = await rig.Pipeline.SummarizeBatchAsync([Doc(1), Doc(2)], TestContext.Current.CancellationToken);

        summaries.Single(s => s.DocumentId == "d1").Summary.ShouldNotBeNull();
        var failed = summaries.Single(s => s.DocumentId == "d2");
        failed.Summary.ShouldBeNull();
        failed.Error.ShouldNotBeNull().ShouldContain("still running");
    }

    [Fact]
    public async Task Failover_KeepsUsingThePrimaryWhileItIsHealthy()
    {
        using var rig = new Rig();
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "P1");
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "P2");

        await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);
        await rig.Pipeline.SummarizeAsync(Doc(2), TestContext.Current.CancellationToken);

        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(2);
        rig.Endpoints.Fallback.RecordedCalls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task UnparseableReply_IsRetriedWithTheParseErrorAndThenSucceeds()
    {
        using var rig = new Rig();
        rig.Endpoints.Primary.EnqueueText("this reply has none of the expected field markers");
        FakeScript.EnqueueSummary(rig.Endpoints.Primary, "repaired answer");

        var answer = await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken);

        answer.ShouldBe("repaired answer");
        var calls = rig.Endpoints.Primary.RecordedCalls;
        calls.Count.ShouldBe(2);
        calls[1].Messages.Count.ShouldBe(calls[0].Messages.Count + 1);
        calls[1].Messages[^1].Role.ShouldBe(ChatRole.User);
        calls[1].Messages[^1].Text.ShouldContain("could not be parsed");
    }

    [Fact]
    public async Task UnparseableReply_GivesUpAfterTheConfiguredRecoveryAttempts()
    {
        using var rig = new Rig(o => o with { ParseRecoveryAttempts = 1 });
        for (var i = 0; i < 3; i++)
            rig.Endpoints.Primary.EnqueueText("still no markers");

        await Should.ThrowAsync<AdapterParseException>(
            async () => await rig.Pipeline.SummarizeAsync(Doc(1), TestContext.Current.CancellationToken));

        rig.Endpoints.Primary.RecordedCalls.Count.ShouldBe(2);
    }

    static async Task<List<SignalGrid.Ampelum.PredictionUpdate<SummarySignature>>> Collect(
        IAsyncEnumerable<SignalGrid.Ampelum.PredictionUpdate<SummarySignature>> source)
    {
        var all = new List<SignalGrid.Ampelum.PredictionUpdate<SummarySignature>>();
        await foreach (var update in source)
            all.Add(update);
        return all;
    }
}
