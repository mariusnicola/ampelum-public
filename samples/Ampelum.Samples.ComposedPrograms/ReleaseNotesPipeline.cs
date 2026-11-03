using System.Collections.Concurrent;
using Ampelum.Samples.ComposedPrograms.Assistant;
using Ampelum.Samples.ComposedPrograms.Drafting;
using Ampelum.Samples.ComposedPrograms.Observability;
using Ampelum.Samples.ComposedPrograms.Review;
using Ampelum.Samples.ComposedPrograms.Style;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SignalGrid.Ampelum;
using SignalGrid.Ampelum.Adapters;
using SignalGrid.Ampelum.Agent;
using SignalGrid.Ampelum.Agents.Memory;
using SignalGrid.Ampelum.Agents.Tools;
using SignalGrid.Ampelum.Orchestration;

namespace Ampelum.Samples.ComposedPrograms;

/// <summary>
/// Release notes from a CSV of changes: every area is drafted in parallel by an orchestrator, then an
/// assembly step writes the headline, has the notes reviewed by a panel and announced by a task agent.
/// </summary>
public sealed partial class ReleaseNotesPipeline : IAsyncDisposable
{
    const string DraftTask = "draft";
    const string AssembleTask = "assemble";

    readonly IChatClient _chat;
    readonly ChangeSource _source;
    readonly ReleaseNotesOptions _options;
    readonly ILoggerFactory _loggers;
    readonly IServiceProvider _services;
    readonly List<ChangeRow> _changes = [];
    readonly System.Lazy<Task<StyleMemory>> _style;
    readonly IDisposable _spans;

    public ReleaseNotesPipeline(
        IChatClient chat, ChangeSource source, ReleaseNotesOptions? options = null, ILoggerFactory? loggers = null,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        _chat = chat;
        _source = source;
        _options = options ?? new ReleaseNotesOptions();
        _loggers = loggers ?? NullLoggerFactory.Instance;
        Traces = new InMemoryTraceSink();
        _services = ReleaseNotesServices.Build(
            chat, _options, _loggers, Traces, new TraceFileExporter(_options.TracePath), _changes, embeddings);
        _style = new System.Lazy<Task<StyleMemory>>(() => StyleMemory.OpenAsync(
            source.DataDirectory, chat, _services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), _options));
        _spans = new SpanListener();
    }

    /// <summary>Every trace record of every scope, collected in memory.</summary>
    public InMemoryTraceSink Traces { get; }

    public IServiceProvider Services => _services;

    public Task<StyleMemory> StyleAsync() => _style.Value;

    public async Task<ReleaseResult> RunAsync(CancellationToken ct = default)
    {
        var rows = await _source.LoadChangesAsync(ct).ConfigureAwait(false);
        _changes.Clear();
        _changes.AddRange(rows);

        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        // Retrievers and the memory tiers read the ambient context to run their calls through the hooks.
        using var ambient = AmpelumContext.Push(ctx);

        var style = await _style.Value.ConfigureAwait(false);
        var drafter = new NoteDrafter(ctx, _options, style, _loggers.CreateLogger<NoteDrafter>());
        var drafted = new ConcurrentDictionary<string, DraftedNote>();

        var areas = rows.Select(r => r.Area).Distinct().ToList();
        var nodes = areas.Select(area => new TaskNode
        {
            Id = $"{DraftTask}-{area}",
            TaskType = DraftTask,
            Description = $"Draft the notes of {area}",
            Metadata = new Dictionary<string, object?> { ["area"] = area },
        }).ToList();
        nodes.Add(new TaskNode
        {
            Id = AssembleTask,
            TaskType = AssembleTask,
            Description = "Headline, review and announcement",
            DependsOn = nodes.Select(n => n.Id).ToHashSet(),
        });

        ReleaseResult? result = null;
        var outcomes = await new SubagentOrchestrator(_loggers.CreateLogger<SubagentOrchestrator>()).ExecuteAsync(
            nodes,
            async (node, token) =>
            {
                if (node.TaskType == DraftTask)
                {
                    var area = (string)node.Metadata["area"]!;
                    foreach (var row in rows.Where(r => r.Area == area))
                        drafted[row.Id] = (await drafter.DraftAsync(row, token).ConfigureAwait(false)).Note;
                    return new TaskExecutionResult { Success = true, Output = $"{area}: drafted" };
                }
                var ordered = rows.Select(r => drafted[r.Id]).ToList();
                result = await AssembleAsync(ctx, scope.ServiceProvider, ordered, token).ConfigureAwait(false);
                return new TaskExecutionResult { Success = true, Output = "assembled" };
            },
            ct).ConfigureAwait(false);

        var failed = outcomes.Where(o => !o.Success).Select(o => o.Error).ToList();
        if (failed.Count > 0 || result is null)
            throw new InvalidOperationException("The release run failed: " + string.Join("; ", failed));
        return result;
    }

    /// <summary>Tags one change with the fields named in the options.</summary>
    public async Task<IReadOnlyDictionary<string, string>> TagAsync(ChangeRow row, CancellationToken ct = default)
    {
        await using var scope = _services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AmpelumContext>();
        return await new ChangeTagger(ctx, _options).TagAsync(row, ct).ConfigureAwait(false);
    }

    async Task<ReleaseResult> AssembleAsync(
        AmpelumContext ctx, IServiceProvider scoped, IReadOnlyList<DraftedNote> notes, CancellationToken ct)
    {
        // Both consumers below ask for the headline; the lazy wrapper makes the model call once.
        var headlineModule = new SignalGrid.Ampelum.Orchestration.Lazy<HeadlineSig>(
            new Predict<HeadlineSig>(ctx with { Adapter = new TwoStepAdapter(_chat) }));
        var headlineInput = new HeadlineSig { Notes = string.Join("\n", notes.Select(n => $"{n.Kind}: {n.Note}")) };

        var review = await new ReviewPanel(
                ctx, _chat, _services.GetRequiredService<IAgentToolRegistry>(),
                _services.GetRequiredService<IConversationMemory>(), _options)
            .ReviewAsync(notes, ct).ConfigureAwait(false);

        var headline = (await headlineModule.ForwardAsync(headlineInput, ct).ConfigureAwait(false)).Value.Headline;
        var again = (await headlineModule.ForwardAsync(headlineInput, ct).ConfigureAwait(false)).Value.Headline;
        var announcement = await new AnnouncementWriter(ctx).WriteAsync(again, notes, ct).ConfigureAwait(false);

        // Breaking changes are the ones a wrong note hurts most, so only they are checked against the record.
        var changes = _changes.ToDictionary(c => c.Id);
        var checks = new Dictionary<string, string>();
        foreach (var note in notes.Where(n => n.Kind == ChangeKind.Breaking))
            checks[note.Id] = (await new FactCheck(ctx, changes, _options.FactCheckIterations).CheckAsync(note, ct).ConfigureAwait(false)).Value.Verdict;

        return new ReleaseResult(notes, headline, review, announcement.Text, checks);
    }

    public async ValueTask DisposeAsync()
    {
        _spans.Dispose();
        if (_services is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);
    }
}
