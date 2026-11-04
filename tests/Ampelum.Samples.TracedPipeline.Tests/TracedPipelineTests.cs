using System.Text;
using System.Text.Json;
using Ampelum.Samples.Shared;
using Ampelum.Samples.TracedPipeline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Exceptions;
using SignalGrid.Ampelum.Optimizers;
using SignalGrid.Ampelum.Testing;
using Xunit;

namespace Ampelum.Samples.TracedPipeline.Tests;

// Serial with any other test that touches the process environment.
[Collection("ProcessEnvironment")]
public sealed class TracedPipelineTests : IDisposable
{
    // Distinct from the scripted settings so a test only passes when the pipeline reads what it was given.
    static readonly TracingSettings Settings = new(
        LangFusePublicKey: "lf-public-under-test",
        LangFuseSecretKey: "lf-secret-under-test",
        LangFuseEndpoint: new Uri("http://langfuse.test"),
        LangSmithApiKey: "ls-key-under-test",
        LangSmithProject: "ls-project-under-test",
        LangSmithEndpoint: new Uri("http://langsmith.test"),
        PhoenixEndpoint: new Uri("http://phoenix.test:6006"),
        PhoenixApiKey: "px-key-under-test");

    // 6 variants (baseline plus 5 optimizers) each evaluated on 8 rows.
    const int ExpectedTraces = 48;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "traced-pipeline-tests-" + Guid.NewGuid().ToString("N"));

    public TracedPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string SystemText(RecordedChatCall call) =>
        call.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? "";

    static async Task<(ScriptedChatClient Client, RecordingHandler Handler, TracedPipelineResult Result)> RunAsync(
        TracingSettings? settings = null, string? slowHost = null)
    {
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var client = ScriptedChatClient.Create(data);
        var handler = new RecordingHandler(uri => uri.Host == slowHost ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero);
        var pipeline = new TracedPipeline(
            client, SentimentData.DefaultPath, settings ?? Settings, handler, new LabelMatch());
        return (client, handler, await pipeline.RunAsync(Ct));
    }

    static double MeanOf(TracedPipelineResult result, string name) =>
        result.Variants.Single(v => v.Name == name).Evaluation.Mean;

    static IEnumerable<JsonElement> LangFuseEvents(RecordingHandler handler) =>
        handler.Requests
            .Where(r => r.Uri.Host == Settings.LangFuseEndpoint.Host)
            .SelectMany(r => JsonDocument.Parse(r.BodyText).RootElement.GetProperty("batch").EnumerateArray()
                .Select(e => e.Clone()));

    static IEnumerable<JsonElement> LangSmithRuns(RecordingHandler handler) =>
        handler.Requests
            .Where(r => r.Uri.Host == Settings.LangSmithEndpoint.Host)
            .SelectMany(r => JsonDocument.Parse(r.BodyText).RootElement.GetProperty("post").EnumerateArray()
                .Select(e => e.Clone()));

    static int Occurrences(byte[] haystack, string needle)
    {
        var bytes = Encoding.UTF8.GetBytes(needle);
        var count = 0;
        for (var i = 0; i + bytes.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, bytes.Length).SequenceEqual(bytes))
            {
                count++;
                i += bytes.Length - 1;
            }
        }
        return count;
    }

    [Fact]
    public async Task Each_optimizer_scores_from_the_prompt_the_model_receives()
    {
        var (_, _, result) = await RunAsync();

        result.RowCount.ShouldBe(8);
        result.Variants.Select(v => v.Name).ShouldBe(
        [
            "baseline", "BootstrapFewShot", "LabeledFewShot",
            "BootstrapFewShotWithRandomSearch", "Copro", "InferRules",
        ]);
        MeanOf(result, "baseline").ShouldBe(3 / 8.0);
        // Two demos taken from the program's own traces: rows 0 and 1, both answered correctly.
        MeanOf(result, "BootstrapFewShot").ShouldBe(5 / 8.0);
        // Four gold-labeled demos.
        MeanOf(result, "LabeledFewShot").ShouldBe(7 / 8.0);
        // The shuffled trial with the most correct demos, not the first trial.
        MeanOf(result, "BootstrapFewShotWithRandomSearch").ShouldBe(6 / 8.0);
        // The second proposal, which only a real scoring run can tell apart from the first.
        MeanOf(result, "Copro").ShouldBe(8 / 8.0);
        MeanOf(result, "InferRules").ShouldBe(6 / 8.0);
        result.Variants.ShouldAllBe(v => v.Evaluation.FailureCount == 0);
        result.ComparisonMarkdown.ShouldContain("| Copro | 8/8 | 1.000 |");
    }

    [Fact]
    public async Task BootstrapFewShot_over_an_unbound_program_fails_naming_the_cause_instead_of_returning_no_demos()
    {
        var data = await SentimentData.LoadAsync(SentimentData.DefaultPath, Ct);
        var plain = new AmpelumContext(
            ScriptedChatClient.Create(data), new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var trainset = data.Take(3).Select(row => Example.From(row.Expected)).ToList();
        // The program is never bound, so its Predict has no context and every run throws.
        var optimizer = new BootstrapFewShot<SentimentProgram>(async (program, example, context, ct) =>
        {
            var prediction = await program.ClassifyAsync(
                new SentimentSig { Feedback = Convert.ToString(example.Inputs[nameof(SentimentSig.Feedback)])! }, ct);
            context.TraceSink.Write(prediction.Trace);
        });

        var failure = await Should.ThrowAsync<OptimizerException>(() =>
            optimizer.CompileAsync(new SentimentProgram(), trainset, new LabelMatch(), plain, ct: Ct));

        failure.Message.ShouldStartWith("BootstrapFewShot: the program runner failed on all 3 examples. First error: ");
        failure.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task Copro_runs_both_proposals_and_the_evaluation_uses_the_winner()
    {
        var (client, _, _) = await RunAsync();

        var calls = client.RecordedCalls;
        calls.Count(c => SystemText(c).Contains(ScriptedChatClient.WeakProposal)).ShouldBe(8);
        // Eight scoring calls while compiling plus eight in the traced evaluation.
        calls.Count(c => SystemText(c).Contains(ScriptedChatClient.BestProposal)).ShouldBe(16);
    }

    [Fact]
    public async Task InferRules_rules_reach_the_classification_prompt()
    {
        var (client, _, _) = await RunAsync();

        client.RecordedCalls.Count(c => SystemText(c).Contains(ScriptedChatClient.Rules)).ShouldBe(8);
    }

    [Fact]
    public async Task Optimizer_runs_are_not_exported_only_the_evaluations()
    {
        var (_, handler, result) = await RunAsync();

        result.TracesExported.ShouldBe(ExpectedTraces);
        LangSmithRuns(handler).Count().ShouldBe(ExpectedTraces);
    }

    [Theory]
    [InlineData("langfuse.test")]
    [InlineData("langsmith.test")]
    public async Task Every_export_has_arrived_when_the_run_returns_even_if_one_endpoint_is_slow(string slowHost)
    {
        // Only one destination is slow, so its flush cannot be hidden by another destination's wait.
        var (_, handler, _) = await RunAsync(slowHost: slowHost);

        LangFuseEvents(handler).Count().ShouldBe(2 * ExpectedTraces);
        LangSmithRuns(handler).Count().ShouldBe(ExpectedTraces);
    }

    [Fact]
    public async Task LangFuse_receives_every_trace_with_basic_auth_from_the_settings()
    {
        var (_, handler, _) = await RunAsync();

        var requests = handler.Requests.Where(r => r.Uri.Host == Settings.LangFuseEndpoint.Host).ToList();
        requests.ShouldNotBeEmpty();
        var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Settings.LangFusePublicKey}:{Settings.LangFuseSecretKey}"));
        foreach (var request in requests)
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.Uri.AbsolutePath.ShouldBe("/api/public/ingestion");
            request.Headers["Authorization"].ShouldBe(expectedAuth);
        }

        var events = LangFuseEvents(handler).ToList();
        events.Count(e => e.GetProperty("type").GetString() == "trace-create").ShouldBe(ExpectedTraces);
        var generations = events.Where(e => e.GetProperty("type").GetString() == "generation-create").ToList();
        generations.Count.ShouldBe(ExpectedTraces);
        foreach (var generation in generations)
        {
            var body = generation.GetProperty("body");
            body.GetProperty("name").GetString().ShouldBe("classify");
            body.GetProperty("metadata").GetProperty("ampelum.module").GetString().ShouldBe("classify");
            body.GetProperty("metadata").GetProperty("ampelum.signature_type").GetString().ShouldBe("SentimentSig");
        }
    }

    [Fact]
    public async Task LangSmith_receives_every_run_with_key_and_project_from_the_settings()
    {
        var (_, handler, _) = await RunAsync();

        var requests = handler.Requests.Where(r => r.Uri.Host == Settings.LangSmithEndpoint.Host).ToList();
        requests.ShouldNotBeEmpty();
        foreach (var request in requests)
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.Uri.AbsolutePath.ShouldBe("/runs/batch");
            request.Headers["X-API-Key"].ShouldBe(Settings.LangSmithApiKey);
            request.Headers["x-project-name"].ShouldBe(Settings.LangSmithProject);
        }

        var runs = LangSmithRuns(handler).ToList();
        runs.Count.ShouldBe(ExpectedTraces);
        foreach (var run in runs)
        {
            run.GetProperty("name").GetString().ShouldBe("classify");
            run.GetProperty("session_name").GetString().ShouldBe(Settings.LangSmithProject);
            var metadata = run.GetProperty("extra").GetProperty("metadata");
            metadata.GetProperty("ampelum.module").GetString().ShouldBe("classify");
            metadata.GetProperty("ampelum.signature_type").GetString().ShouldBe("SentimentSig");
        }
    }

    [Fact]
    public async Task Phoenix_spans_reach_the_collector_over_otlp_with_the_key_from_the_settings()
    {
        var (_, handler, _) = await RunAsync();

        var requests = handler.Requests.Where(r => r.Uri.Host == Settings.PhoenixEndpoint.Host).ToList();
        requests.ShouldNotBeEmpty();
        foreach (var request in requests)
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.Uri.AbsolutePath.ShouldBe("/v1/traces");
            request.Uri.Port.ShouldBe(Settings.PhoenixEndpoint.Port);
            request.Headers["api_key"].ShouldBe(Settings.PhoenixApiKey);
        }

        // The body is protobuf, in which strings are plain UTF-8.
        var body = requests.SelectMany(r => r.Body).ToArray();
        Occurrences(body, "openinference.span.kind").ShouldBe(ExpectedTraces);
        Occurrences(body, "ampelum.module").ShouldBe(ExpectedTraces);
        Occurrences(body, "ampelum.signature_type").ShouldBe(ExpectedTraces);
        Occurrences(body, "SentimentSig").ShouldBe(ExpectedTraces);
        Occurrences(body, "classify").ShouldBeGreaterThanOrEqualTo(ExpectedTraces);
    }

    [Fact]
    public async Task A_phoenix_without_a_key_sends_no_api_key_header()
    {
        var (_, handler, _) = await RunAsync(Settings with { PhoenixApiKey = null });

        var requests = handler.Requests.Where(r => r.Uri.Host == Settings.PhoenixEndpoint.Host).ToList();
        requests.ShouldNotBeEmpty();
        requests.ShouldAllBe(r => !r.Headers.ContainsKey("api_key"));
    }

    static readonly string[] SettingNames =
    [
        "LANGFUSE_PUBLIC_KEY", "LANGFUSE_SECRET_KEY", "LANGFUSE_ENDPOINT",
        "LANGSMITH_API_KEY", "LANGSMITH_PROJECT", "LANGSMITH_ENDPOINT",
        "PHOENIX_ENDPOINT", "PHOENIX_API_KEY",
    ];

    [Fact]
    public void Settings_are_read_from_the_config_and_a_missing_key_is_named()
    {
        // SampleConfig layers environment variables over .env, so the machine's values must not leak in.
        var saved = SettingNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in SettingNames)
                Environment.SetEnvironmentVariable(name, null);
            SettingsFromConfig();
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    void SettingsFromConfig()
    {
        var env = Path.Combine(_dir, ".env");
        var lines = new[]
        {
            "LANGFUSE_PUBLIC_KEY=a-public", "LANGFUSE_SECRET_KEY=a-secret", "LANGFUSE_ENDPOINT=http://lf.example",
            "LANGSMITH_API_KEY=a-ls-key", "LANGSMITH_PROJECT=a-project", "LANGSMITH_ENDPOINT=http://ls.example",
            "PHOENIX_ENDPOINT=http://px.example:6006", "PHOENIX_API_KEY=a-px-key",
        };
        File.WriteAllLines(env, lines);

        var settings = TracingSettings.Load(SampleConfig.Load(_dir));

        settings.LangFusePublicKey.ShouldBe("a-public");
        settings.LangFuseSecretKey.ShouldBe("a-secret");
        settings.LangFuseEndpoint.ShouldBe(new Uri("http://lf.example"));
        settings.LangSmithApiKey.ShouldBe("a-ls-key");
        settings.LangSmithProject.ShouldBe("a-project");
        settings.LangSmithEndpoint.ShouldBe(new Uri("http://ls.example"));
        settings.PhoenixEndpoint.ShouldBe(new Uri("http://px.example:6006"));
        settings.PhoenixApiKey.ShouldBe("a-px-key");

        File.WriteAllLines(env, lines.Where(l => !l.StartsWith("LANGSMITH_PROJECT", StringComparison.Ordinal)));
        Should.Throw<InvalidOperationException>(() => TracingSettings.Load(SampleConfig.Load(_dir)))
            .Message.ShouldContain("LANGSMITH_PROJECT");

        File.WriteAllLines(env, lines.Where(l => !l.StartsWith("PHOENIX_API_KEY", StringComparison.Ordinal)));
        TracingSettings.Load(SampleConfig.Load(_dir)).PhoenixApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_dataset_fails_before_any_model_call_or_request()
    {
        var client = new FakeChatClient();
        var handler = new RecordingHandler();
        var pipeline = new TracedPipeline(
            client, Path.Combine(_dir, "nope.jsonl"), Settings, handler, new LabelMatch());

        await Should.ThrowAsync<FileNotFoundException>(() => pipeline.RunAsync(Ct));

        client.RecordedCalls.ShouldBeEmpty();
        handler.Requests.ShouldBeEmpty();
    }
}
