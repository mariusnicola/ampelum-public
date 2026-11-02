using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.DependencyInjection;
using SignalGrid.Ampelum.Evaluation;
using SignalGrid.Ampelum.Observability.LangFuse;
using SignalGrid.Ampelum.Observability.LangSmith;
using SignalGrid.Ampelum.Observability.Phoenix;
using SignalGrid.Ampelum.Optimizers;

namespace Ampelum.Samples.TracedPipeline;

/// <summary>One program variant and how it scored on the labeled data.</summary>
/// <param name="Name">Optimizer name, or "baseline" for the untuned program.</param>
/// <param name="Evaluation">Evaluation of the variant on every labeled row.</param>
/// <param name="Note">What the optimizer does or ignores.</param>
public sealed record VariantResult(string Name, EvaluationResult<SentimentSig> Evaluation, string Note);

/// <summary>Everything the pipeline measured and exported.</summary>
/// <param name="Variants">Baseline first, then each optimizer, all on the same rows.</param>
/// <param name="RowCount">Labeled rows each variant was evaluated on.</param>
/// <param name="TracesExported">Traces handed to the three exporters.</param>
/// <param name="ComparisonMarkdown">The variants as a markdown table.</param>
public sealed record TracedPipelineResult(
    IReadOnlyList<VariantResult> Variants,
    int RowCount,
    int TracesExported,
    string ComparisonMarkdown);

/// <summary>
/// Evaluates a program tuned by several optimizers on the same labeled data and exports the
/// evaluation traces to LangFuse, LangSmith and Phoenix.
/// </summary>
public sealed class TracedPipeline(
    IChatClient chat,
    string datasetPath,
    TracingSettings tracing,
    HttpMessageHandler? exportHandler,
    LabelMatch metric)
{
    // Demo caps differ so the variants stay distinguishable in the report.
    const int BootstrapDemos = 2;
    const int LabeledDemos = 4;
    const int SearchTrials = 3;
    const int SearchDemos = 3;
    const int CoproCandidates = 2;
    // Fixed so the random search shuffles the same way on every run.
    const int SearchSeed = 4;

    /// <summary>Runs every variant; every exporter has flushed when the task completes.</summary>
    public async Task<TracedPipelineResult> RunAsync(CancellationToken ct = default)
    {
        var data = await SentimentData.LoadAsync(datasetPath, ct).ConfigureAwait(false);
        var trainset = data.Select(e => Example.From(e.Expected)).ToList();

        // Optimizer runs use a context that exports nothing, so only the evaluations below are traced.
        var plain = new AmpelumContext(chat, new ChatAdapter(), NullTraceSink.Instance, NullLoggerFactory.Instance);
        var untuned = new OptimizedProgram<SentimentProgram>(
            new SentimentProgram(),
            new Dictionary<string, IReadOnlyList<Example>>(),
            new Dictionary<string, string>());
        var bound = untuned.Bind(plain);

        var compiled = await CompileAllAsync(untuned, bound, trainset, plain, ct).ConfigureAwait(false);

        // One provider per destination: DI decoration disposes, and so flushes, only the outermost exporter.
        // The scopes are disposed when this method returns, which drains each exporter queue before the
        // caller sees the result.
        await using var langFuse = BuildSinkScope(
            b => b.AddLangFuseExporter(
                c =>
                {
                    c.PublicKey = tracing.LangFusePublicKey;
                    c.SecretKey = tracing.LangFuseSecretKey;
                    c.Endpoint = tracing.LangFuseEndpoint;
                }));
        await using var langSmith = BuildSinkScope(
            b => b.AddLangSmithExporter(
                c =>
                {
                    c.ApiKey = tracing.LangSmithApiKey;
                    c.ProjectName = tracing.LangSmithProject;
                    c.Endpoint = tracing.LangSmithEndpoint;
                }));
        await using var phoenix = BuildSinkScope(
            b => b.AddPhoenixExporter(
                c =>
                {
                    c.Endpoint = tracing.PhoenixEndpoint;
                    c.ApiKey = tracing.PhoenixApiKey;
                }));

        var fanOut = new FanOutTraceSink(
        [
            langFuse.ServiceProvider.GetRequiredService<ITraceSink>(),
            langSmith.ServiceProvider.GetRequiredService<ITraceSink>(),
            phoenix.ServiceProvider.GetRequiredService<ITraceSink>(),
        ]);
        var traced = new AmpelumContext(chat, new ChatAdapter(), fanOut, NullLoggerFactory.Instance);

        var variants = new List<VariantResult>();
        using (CreatePhoenixTracerProvider())
        {
            foreach (var (name, program, note) in compiled)
            {
                var evaluation = await MeasureAsync(program.Bind(traced), data, ct).ConfigureAwait(false);
                variants.Add(new VariantResult(name, evaluation, note));
            }
        }

        return new TracedPipelineResult(variants, data.Count, fanOut.Count, Render(variants, data.Count));
    }

    async Task<List<(string Name, OptimizedProgram<SentimentProgram> Program, string Note)>> CompileAllAsync(
        OptimizedProgram<SentimentProgram> untuned,
        SentimentProgram bound,
        IReadOnlyList<Example> trainset,
        AmpelumContext plain,
        CancellationToken ct)
    {
        // BootstrapFewShot only collects traces and never calls the metric. The scoring runner given to
        // the others is what makes their metric score the program instead of the data against itself.
        var bootstrap = await new BootstrapFewShot<SentimentProgram>(CollectTraceAsync, BootstrapDemos)
            .CompileAsync(bound, trainset, metric, plain, ct: ct).ConfigureAwait(false);
        var labeled = await new LabeledFewShot<SentimentProgram>(LabeledDemos)
            .CompileAsync(bound, trainset, metric, plain, ct: ct).ConfigureAwait(false);
        var search = await new BootstrapFewShotWithRandomSearch<SentimentProgram>(
                CollectTraceAsync, SearchTrials, SearchDemos, SearchSeed, ScoreOneAsync)
            .CompileAsync(bound, trainset, metric, plain, ct: ct).ConfigureAwait(false);
        var copro = await new Copro<SentimentProgram>(chat, CoproCandidates, ScoreOneAsync)
            .CompileAsync(bound, trainset, metric, plain, ct: ct).ConfigureAwait(false);
        var rules = await new InferRules<SentimentProgram>(chat)
            .CompileAsync(bound, trainset, metric, plain, ct: ct).ConfigureAwait(false);

        return
        [
            ("baseline", untuned, "untuned program"),
            ("BootstrapFewShot", bootstrap, "demos from the program's own traces; the metric is ignored"),
            ("LabeledFewShot", labeled, "first labeled rows as demos; no model call"),
            ("BootstrapFewShotWithRandomSearch", search, "best of shuffled bootstraps, scored by running the program"),
            ("Copro", copro, "best proposed instruction, scored by running the program"),
            ("InferRules", rules, "rules written by the model, no scoring"),
        ];
    }

    AsyncServiceScope BuildSinkScope(Action<AmpelumBuilder> addExporter)
    {
        var services = new ServiceCollection();
        addExporter(services.AddAmpelum());
        if (exportHandler is not null)
            services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => exportHandler));
        return services.BuildServiceProvider().CreateAsyncScope();
    }

    // Phoenix delivery is the host's job: the exporter only emits spans on an ActivitySource.
    TracerProvider CreatePhoenixTracerProvider() =>
        Sdk.CreateTracerProviderBuilder()
            .AddSource(PhoenixActivitySource.Name)
            .AddOtlpExporter(o =>
            {
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
                o.Endpoint = new Uri(tracing.PhoenixEndpoint.AbsoluteUri.TrimEnd('/') + "/v1/traces");
                if (tracing.PhoenixApiKey is not null)
                    o.Headers = "api_key=" + tracing.PhoenixApiKey;
                if (exportHandler is not null)
                    o.HttpClientFactory = () => new HttpClient(exportHandler, disposeHandler: false);
            })
            .Build()!;

    // One at a time so a scripted client answers in dataset order.
    static Task<EvaluationResult<SentimentSig>> MeasureAsync(
        SentimentProgram program, IReadOnlyList<LabeledExample<SentimentSig>> data, CancellationToken ct) =>
        new Evaluate<SentimentSig>(program.ClassifyAsync, new LabelMatch(), maxDegreeOfParallelism: 1)
            .RunAsync(data, ct);

    // The bound program writes to its own sink, so the trace is passed on to the one the optimizer reads.
    static async Task CollectTraceAsync(
        SentimentProgram program, Example example, AmpelumContext context, CancellationToken ct)
    {
        var prediction = await program.ClassifyAsync(InputOf(example), ct).ConfigureAwait(false);
        context.TraceSink.Write(prediction.Trace);
    }

    static async Task<Example> ScoreOneAsync(
        SentimentProgram program, Example example, AmpelumContext context, CancellationToken ct)
    {
        var prediction = await program.ClassifyAsync(InputOf(example), ct).ConfigureAwait(false);
        return Example.From(prediction.Value);
    }

    static SentimentSig InputOf(Example example) => new()
    {
        Feedback = Convert.ToString(
            example.Inputs[nameof(SentimentSig.Feedback)], CultureInfo.InvariantCulture) ?? "",
    };

    static string Render(IReadOnlyList<VariantResult> variants, int rows)
    {
        var text = new StringBuilder();
        text.AppendLine("| variant | correct | mean | note |");
        text.AppendLine("|---|---|---|---|");
        foreach (var v in variants)
        {
            var correct = (int)Math.Round(v.Evaluation.Mean * rows);
            text.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {v.Name} | {correct}/{rows} | {v.Evaluation.Mean:0.000} | {v.Note} |"));
        }
        return text.ToString();
    }
}
